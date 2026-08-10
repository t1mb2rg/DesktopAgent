using System.Net;
using System.Text;
using System.Text.Json;
using DesktopAgent.Application.Intelligence;
using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.Intelligence;

namespace DesktopAgent.Tests.Intelligence;

public sealed class OpenAiCompatibleDesktopNaturalLanguageRuleProviderTests
{
    [Fact]
    public async Task Provider_CurrentItemRule_BindsStableIdLocallyAndNeverSendsIt()
    {
        var zoneId = Guid.NewGuid();
        var templateId = Guid.NewGuid();
        string? capturedBody = null;
        using var client = CreateClient(async (request, cancellationToken) =>
        {
            capturedBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return JsonResponse(CreateCompletionJson(
                $$"""{"name":"这个报告","condition":{"match_current_item":true,"file_extension":null,"file_name_pattern":null,"item_kind":null},"category":"Course","target_zone_id":"{{zoneId}}","reason":"用户指定当前项目"}"""));
        });
        var provider = CreateProvider(client);
        var reference = CreateFacts();

        var result = await provider.GenerateRuleDraftAsync(
            new DesktopNaturalLanguageRuleRequest(
                "这个文件以后都放课程区",
                templateId,
                new[] { new IntelligenceZoneOption(zoneId, "课程区") },
                reference));

        Assert.Equal(IntelligenceProviderStatus.Success, result.Status);
        var draft = Assert.IsType<DesktopClassificationRuleDraft>(result.Draft);
        Assert.Equal(reference.StableId, draft.Condition.StableId);
        Assert.Equal(templateId, draft.TargetTemplateId);
        Assert.Equal(zoneId, draft.TargetZoneId);

        using var requestJson = JsonDocument.Parse(capturedBody!);
        var userPrompt = requestJson.RootElement
            .GetProperty("messages")[1]
            .GetProperty("content")
            .GetString()!;
        Assert.DoesNotContain(reference.StableId, userPrompt, StringComparison.Ordinal);
        Assert.Contains("实验报告.pdf", userPrompt, StringComparison.Ordinal);

        var confirmed = draft.Confirm(Guid.NewGuid());
        Assert.Equal(DesktopClassificationRuleAuthority.ExplicitUser, confirmed.Authority);
        Assert.Equal(DesktopClassificationRuleProvenance.AiProposedAndConfirmed, confirmed.Provenance);
    }

    [Fact]
    public async Task Provider_BoundedDsl_MapsExtensionGlobAndItemKind()
    {
        using var client = CreateClient((_, _) => Task.FromResult(JsonResponse(CreateCompletionJson(
            """{"name":"实验报告","condition":{"match_current_item":false,"file_extension":".PDF","file_name_pattern":"*实验报告*.pdf","item_kind":"File"},"category":"Course","target_zone_id":null,"reason":"按文件名和类型归类"}"""))));
        var provider = CreateProvider(client);

        var result = await provider.GenerateRuleDraftAsync(
            new DesktopNaturalLanguageRuleRequest(
                "所有文件名里有实验报告的 PDF 都归类为 Course",
                Guid.NewGuid()));

        var draft = Assert.IsType<DesktopClassificationRuleDraft>(result.Draft);
        Assert.Equal(".pdf", draft.Condition.FileExtension);
        Assert.Equal("*实验报告*.pdf", draft.Condition.FileNamePattern);
        Assert.Equal(ItemKind.File, draft.Condition.ItemKind);
        Assert.Null(draft.Condition.StableId);
        Assert.Null(draft.TargetZoneId);
    }

    [Theory]
    [InlineData("{\"name\":\"x\",\"condition\":{\"match_current_item\":false,\"file_extension\":\".pdf\",\"file_name_pattern\":null,\"item_kind\":null,\"regex\":\".*\"},\"category\":\"Doc\",\"target_zone_id\":null,\"reason\":\"x\"}")]
    [InlineData("{\"name\":\"x\",\"condition\":{\"match_current_item\":false,\"file_extension\":null,\"file_name_pattern\":null,\"item_kind\":null},\"category\":\"Doc\",\"target_zone_id\":null,\"reason\":\"x\"}")]
    public async Task Provider_RejectsExtraFieldsAndEmptyCondition(string payload)
    {
        using var client = CreateClient((_, _) => Task.FromResult(JsonResponse(CreateCompletionJson(payload))));
        var provider = CreateProvider(client);

        var result = await provider.GenerateRuleDraftAsync(
            new DesktopNaturalLanguageRuleRequest("测试规则", Guid.NewGuid()));

        Assert.Equal(IntelligenceProviderStatus.InvalidResponse, result.Status);
        Assert.Null(result.Draft);
    }

    [Fact]
    public async Task Provider_RejectsUnknownZoneAndCurrentItemWithoutReference()
    {
        var knownZone = Guid.NewGuid();
        var unknownZone = Guid.NewGuid();
        var responses = new Queue<HttpResponseMessage>(new[]
        {
            JsonResponse(CreateCompletionJson(
                $$"""{"name":"x","condition":{"match_current_item":false,"file_extension":".pdf","file_name_pattern":null,"item_kind":null},"category":"Doc","target_zone_id":"{{unknownZone}}","reason":"x"}""")),
            JsonResponse(CreateCompletionJson(
                """{"name":"x","condition":{"match_current_item":true,"file_extension":null,"file_name_pattern":null,"item_kind":null},"category":"Doc","target_zone_id":null,"reason":"x"}""")),
        });
        using var client = CreateClient((_, _) => Task.FromResult(responses.Dequeue()));
        var provider = CreateProvider(client);
        var request = new DesktopNaturalLanguageRuleRequest(
            "规则",
            Guid.NewGuid(),
            new[] { new IntelligenceZoneOption(knownZone, "已知") });

        var unknown = await provider.GenerateRuleDraftAsync(request);
        var noReference = await provider.GenerateRuleDraftAsync(request);

        Assert.Equal(IntelligenceProviderStatus.InvalidResponse, unknown.Status);
        Assert.Equal(IntelligenceProviderStatus.InvalidResponse, noReference.Status);
    }

    [Fact]
    public void Request_BoundsInstructionAndZoneEnumeration()
    {
        Assert.Throws<ArgumentException>(() => new DesktopNaturalLanguageRuleRequest(
            new string('x', DesktopNaturalLanguageRuleRequest.MaxInstructionLength + 1),
            Guid.NewGuid()));

        IEnumerable<IntelligenceZoneOption> TooMany()
        {
            for (var i = 0; i <= DesktopClassificationPolicyResolver.MaxZonesPerResolution; i++)
                yield return new IntelligenceZoneOption(Guid.NewGuid(), $"Z{i}");
            throw new InvalidOperationException("Max+1 后不应继续枚举。");
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => new DesktopNaturalLanguageRuleRequest(
            "rule",
            Guid.NewGuid(),
            TooMany()));
    }

    private static DesktopItemFacts CreateFacts()
        => new(
            "private-stable-id",
            "实验报告.pdf",
            ItemKind.File,
            ".pdf",
            isSystemItem: false,
            isFileSystemItem: true);

    private static OpenAiCompatibleDesktopNaturalLanguageRuleProvider CreateProvider(HttpClient client)
        => new(
            client,
            new OpenAiCompatibleProviderOptions(new Uri("https://example.test/v1"), "model"),
            new StubSecretResolver());

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

    private static string CreateCompletionJson(string messageContent)
        => JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content = messageContent } } },
        });

    private sealed class StubSecretResolver : IIntelligenceSecretResolver
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
