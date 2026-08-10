using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DesktopAgent.Application.Intelligence;
using DesktopAgent.Core.DesktopIcons;

namespace DesktopAgent.Tests.Intelligence;

public sealed class OpenAiCompatibleDesktopIntelligenceProviderTests
{
    [Fact]
    public void Options_ValidateTransportBoundaryWithoutRequiringLocalAuth()
    {
        var cloud = new OpenAiCompatibleProviderOptions(
            new Uri("https://api.deepseek.com"),
            "deepseek-v4-pro",
            "credential:deepseek");
        var local = new OpenAiCompatibleProviderOptions(
            new Uri("http://127.0.0.1:8000/v1"),
            "Qwen2.5-7B-Instruct");

        Assert.Equal("credential:deepseek", cloud.SecretReference);
        Assert.Null(local.SecretReference);
        Assert.Equal(TimeSpan.FromSeconds(30), cloud.Timeout);

        Assert.Throws<ArgumentException>(() =>
            new OpenAiCompatibleProviderOptions(new Uri("ftp://example.test"), "model"));
        Assert.Throws<ArgumentException>(() =>
            new OpenAiCompatibleProviderOptions(new Uri("https://example.test/v1?q=1"), "model"));
        Assert.Throws<ArgumentException>(() =>
            new OpenAiCompatibleProviderOptions(new Uri("https://example.test"), " "));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new OpenAiCompatibleProviderOptions(
                new Uri("https://example.test"), "model", timeout: TimeSpan.FromMinutes(6)));
    }

    [Fact]
    public void ClassificationRequest_RejectsDuplicateZoneIds()
    {
        var zoneId = Guid.NewGuid();
        var item = CreateFacts();

        Assert.Throws<ArgumentException>(() => new DesktopItemClassificationRequest(
            item,
            new[]
            {
                new IntelligenceZoneOption(zoneId, "开发"),
                new IntelligenceZoneOption(zoneId, "另一个名字"),
            }));
    }

    [Fact]
    public async Task Provider_Success_UsesConservativeCompatibleRequestAndMapsSuggestion()
    {
        var zoneId = Guid.NewGuid();
        HttpRequestMessage? capturedRequest = null;
        string? capturedBody = null;

        using var httpClient = CreateClient(async (request, cancellationToken) =>
        {
            capturedRequest = request;
            capturedBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return JsonResponse(CreateCompletionJson(
                $$"""{"category":"Development","suggested_zone_id":"{{zoneId}}","confidence":0.91,"reason":"开发工具"}"""));
        });
        var provider = CreateProvider(
            httpClient,
            new OpenAiCompatibleProviderOptions(
                new Uri("https://api.deepseek.com"),
                "deepseek-v4-pro",
                "secret-ref"),
            "test-key");

        var result = await provider.ClassifyAsync(new DesktopItemClassificationRequest(
            CreateFacts(),
            new[] { new IntelligenceZoneOption(zoneId, "开发") }));

        Assert.Equal(IntelligenceProviderStatus.Success, result.Status);
        Assert.NotNull(result.Suggestion);
        Assert.Equal("Development", result.Suggestion.Category);
        Assert.Equal(zoneId, result.Suggestion.SuggestedZoneId);
        Assert.Equal(0.91, result.Suggestion.Confidence);
        Assert.Equal("开发工具", result.Suggestion.Reason);

        Assert.NotNull(capturedRequest);
        Assert.Equal("https://api.deepseek.com/chat/completions", capturedRequest.RequestUri!.AbsoluteUri);
        Assert.Equal(new AuthenticationHeaderValue("Bearer", "test-key"), capturedRequest.Headers.Authorization);

        using var requestJson = JsonDocument.Parse(capturedBody!);
        var root = requestJson.RootElement;
        Assert.Equal("deepseek-v4-pro", root.GetProperty("model").GetString());
        Assert.Equal("json_object", root.GetProperty("response_format").GetProperty("type").GetString());
        Assert.False(root.GetProperty("stream").GetBoolean());
        Assert.Equal(0, root.GetProperty("temperature").GetInt32());

        var messages = root.GetProperty("messages");
        Assert.Equal(2, messages.GetArrayLength());
        var userContent = messages[1].GetProperty("content").GetString()!;
        Assert.Contains("实验报告.pdf", userContent, StringComparison.Ordinal);
        Assert.Contains(zoneId.ToString(), userContent, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stable-private-id", userContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Provider_LocalNoAuth_SendsNoAuthorizationHeader()
    {
        AuthenticationHeaderValue? authorization = new("Bearer", "unexpected");
        using var httpClient = CreateClient((request, _) =>
        {
            authorization = request.Headers.Authorization;
            return Task.FromResult(JsonResponse(CreateCompletionJson(
                """{"category":"Document","suggested_zone_id":null,"confidence":0.8,"reason":"文档"}""")));
        });
        var provider = CreateProvider(
            httpClient,
            new OpenAiCompatibleProviderOptions(
                new Uri("http://127.0.0.1:8000/v1"),
                "local-model"),
            secret: null);

        var result = await provider.ClassifyAsync(new DesktopItemClassificationRequest(CreateFacts()));

        Assert.Equal(IntelligenceProviderStatus.Success, result.Status);
        Assert.Null(authorization);
    }

    [Fact]
    public async Task Provider_MissingReferencedSecret_StopsBeforeHttp()
    {
        var httpReached = false;
        using var httpClient = CreateClient((_, _) =>
        {
            httpReached = true;
            return Task.FromResult(JsonResponse("{}"));
        });
        var provider = CreateProvider(
            httpClient,
            new OpenAiCompatibleProviderOptions(
                new Uri("https://example.test/v1"),
                "model",
                "missing-secret"),
            secret: null);

        var result = await provider.ClassifyAsync(new DesktopItemClassificationRequest(CreateFacts()));

        Assert.Equal(IntelligenceProviderStatus.NotConfigured, result.Status);
        Assert.False(httpReached);
    }

    [Fact]
    public async Task Provider_InvalidPayloads_AreRejectedLocally()
    {
        var knownZone = Guid.NewGuid();
        var unknownZone = Guid.NewGuid();
        var responses = new Queue<HttpResponseMessage>(new[]
        {
            JsonResponse(CreateCompletionJson("not-json")),
            JsonResponse(CreateCompletionJson(
                $$"""{"category":"Development","suggested_zone_id":"{{unknownZone}}","confidence":0.9,"reason":"开发"}""")),
        });
        using var httpClient = CreateClient((_, _) => Task.FromResult(responses.Dequeue()));
        var provider = CreateProvider(
            httpClient,
            new OpenAiCompatibleProviderOptions(new Uri("https://example.test/v1"), "model"),
            secret: null);
        var request = new DesktopItemClassificationRequest(
            CreateFacts(),
            new[] { new IntelligenceZoneOption(knownZone, "开发") });

        var malformed = await provider.ClassifyAsync(request);
        var unknown = await provider.ClassifyAsync(request);

        Assert.Equal(IntelligenceProviderStatus.InvalidResponse, malformed.Status);
        Assert.Equal(IntelligenceProviderStatus.InvalidResponse, unknown.Status);
        Assert.Null(malformed.Suggestion);
        Assert.Null(unknown.Suggestion);
    }

    [Fact]
    public async Task Provider_HttpFailureAndOversizedResponse_DegradeWithoutSuggestion()
    {
        var responses = new Queue<HttpResponseMessage>(new[]
        {
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            JsonResponse(new string('x', 256 * 1024 + 1)),
        });
        using var httpClient = CreateClient((_, _) => Task.FromResult(responses.Dequeue()));
        var provider = CreateProvider(
            httpClient,
            new OpenAiCompatibleProviderOptions(new Uri("https://example.test/v1"), "model"),
            secret: null);
        var request = new DesktopItemClassificationRequest(CreateFacts());

        var unavailable = await provider.ClassifyAsync(request);
        var oversized = await provider.ClassifyAsync(request);

        Assert.Equal(IntelligenceProviderStatus.Unavailable, unavailable.Status);
        Assert.Equal((int)HttpStatusCode.ServiceUnavailable, unavailable.HttpStatusCode);
        Assert.Equal(IntelligenceProviderStatus.InvalidResponse, oversized.Status);
        Assert.Null(unavailable.Suggestion);
        Assert.Null(oversized.Suggestion);
    }

    [Fact]
    public async Task Provider_TimeoutAndCallerCancellation_AreDistinct()
    {
        using var httpClient = CreateClient(async (_, cancellationToken) =>
        {
            await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        });

        var timeoutProvider = CreateProvider(
            httpClient,
            new OpenAiCompatibleProviderOptions(
                new Uri("https://example.test/v1"),
                "model",
                timeout: TimeSpan.FromMilliseconds(50)),
            secret: null);
        var timeoutResult = await timeoutProvider.ClassifyAsync(
            new DesktopItemClassificationRequest(CreateFacts()));

        using var callerCts = new CancellationTokenSource();
        callerCts.Cancel();
        var cancelledProvider = CreateProvider(
            httpClient,
            new OpenAiCompatibleProviderOptions(new Uri("https://example.test/v1"), "model"),
            secret: null);
        var cancelledResult = await cancelledProvider.ClassifyAsync(
            new DesktopItemClassificationRequest(CreateFacts()), callerCts.Token);

        Assert.Equal(IntelligenceProviderStatus.Timeout, timeoutResult.Status);
        Assert.Equal(IntelligenceProviderStatus.Cancelled, cancelledResult.Status);
    }

    private static DesktopItemFacts CreateFacts()
        => new(
            "stable-private-id",
            "实验报告.pdf",
            ItemKind.File,
            ".pdf",
            isSystemItem: false,
            isFileSystemItem: true);

    private static OpenAiCompatibleDesktopIntelligenceProvider CreateProvider(
        HttpClient httpClient,
        OpenAiCompatibleProviderOptions options,
        string? secret)
        => new(httpClient, options, new StubSecretResolver(secret));

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
        private readonly string? _secret;

        public StubSecretResolver(string? secret) => _secret = secret;

        public ValueTask<string?> ResolveSecretAsync(
            string secretReference,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(_secret);
    }

    private sealed class DelegateHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

        public DelegateHandler(
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
            => _handler = handler;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => _handler(request, cancellationToken);
    }
}
