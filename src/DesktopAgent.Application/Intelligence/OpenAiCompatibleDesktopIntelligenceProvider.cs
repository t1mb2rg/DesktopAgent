using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DesktopAgent.Core.Intelligence;

namespace DesktopAgent.Application.Intelligence;

/// <summary>
/// 使用 OpenAI-compatible Chat Completions 子集完成桌面项目语义分类。
/// 该 Provider 只输出 DesktopItemClassificationSuggestion，不接触物理坐标、Shell 或 Writer。
/// </summary>
public sealed class OpenAiCompatibleDesktopIntelligenceProvider : IDesktopIntelligenceProvider
{
    private const int MaxResponseBytes = 256 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private const string SystemPrompt = """
You are the semantic classification component of DesktopAgent.
Return JSON only. Never output physical coordinates, shell commands, file move/delete commands, scripts, or executable actions.
Classify the desktop item using only the supplied facts and user-visible logical zones.
If you recommend a zone, suggested_zone_id must be exactly one of the supplied zone_id values. Otherwise use null.
The JSON object must have exactly this semantic shape:
{
  "category": "non-empty string",
  "suggested_zone_id": "existing zone GUID or null",
  "confidence": 0.0,
  "reason": "concise non-empty explanation"
}
confidence must be between 0 and 1 inclusive.
""";

    private readonly HttpClient _httpClient;
    private readonly OpenAiCompatibleProviderOptions _options;
    private readonly IIntelligenceSecretResolver _secretResolver;

    public OpenAiCompatibleDesktopIntelligenceProvider(
        HttpClient httpClient,
        OpenAiCompatibleProviderOptions options,
        IIntelligenceSecretResolver secretResolver)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _secretResolver = secretResolver ?? throw new ArgumentNullException(nameof(secretResolver));
    }

    public async Task<IntelligenceProviderResult> ClassifyAsync(
        DesktopItemClassificationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        string? apiKey = null;
        if (_options.SecretReference is not null)
        {
            try
            {
                apiKey = await _secretResolver
                    .ResolveSecretAsync(_options.SecretReference, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return IntelligenceProviderResult.Failure(
                    IntelligenceProviderStatus.Cancelled,
                    "解析 Provider Secret 时调用被取消。");
            }

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                return IntelligenceProviderResult.Failure(
                    IntelligenceProviderStatus.NotConfigured,
                    "配置引用了 API Secret，但当前 Secret Resolver 没有返回可用值。");
            }
        }

        using var message = BuildRequestMessage(request, apiKey);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_options.Timeout);

        try
        {
            using var response = await _httpClient.SendAsync(
                    message,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeoutCts.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return IntelligenceProviderResult.Failure(
                    IntelligenceProviderStatus.Unavailable,
                    $"Provider 返回 HTTP {(int)response.StatusCode}。",
                    (int)response.StatusCode);
            }

            var body = await ReadLimitedUtf8Async(response.Content, timeoutCts.Token)
                .ConfigureAwait(false);
            if (body is null)
            {
                return IntelligenceProviderResult.Failure(
                    IntelligenceProviderStatus.InvalidResponse,
                    $"Provider 响应超过 {MaxResponseBytes} bytes 上限。");
            }

            return ParseResponse(body, request);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return IntelligenceProviderResult.Failure(
                IntelligenceProviderStatus.Cancelled,
                "Provider 调用被调用方取消。");
        }
        catch (OperationCanceledException)
        {
            return IntelligenceProviderResult.Failure(
                IntelligenceProviderStatus.Timeout,
                $"Provider 调用超过 {_options.Timeout.TotalSeconds:0.###} 秒超时。");
        }
        catch (HttpRequestException ex)
        {
            return IntelligenceProviderResult.Failure(
                IntelligenceProviderStatus.Unavailable,
                $"Provider 网络请求失败：{ex.Message}");
        }
    }

    private HttpRequestMessage BuildRequestMessage(
        DesktopItemClassificationRequest request,
        string? apiKey)
    {
        var itemContext = new
        {
            item = new
            {
                display_name = request.Item.DisplayName,
                file_extension = request.Item.FileExtension,
                item_kind = request.Item.ItemKind.ToString(),
                is_system_item = request.Item.IsSystemItem,
                is_file_system_item = request.Item.IsFileSystemItem,
            },
            zones = request.Zones.Select(static zone => new
            {
                zone_id = zone.ZoneId,
                name = zone.Name,
            }).ToArray(),
        };

        var requestBody = new
        {
            model = _options.Model,
            messages = new object[]
            {
                new { role = "system", content = SystemPrompt },
                new
                {
                    role = "user",
                    content = "Classify this desktop item. Input JSON:\n" +
                              JsonSerializer.Serialize(itemContext, JsonOptions),
                },
            },
            response_format = new { type = "json_object" },
            stream = false,
            temperature = 0,
        };

        var endpoint = new Uri(
            _options.BaseUri.AbsoluteUri.TrimEnd('/') + "/chat/completions",
            UriKind.Absolute);

        var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(requestBody, JsonOptions),
                Encoding.UTF8,
                "application/json"),
        };
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (apiKey is not null)
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        return message;
    }

    private static IntelligenceProviderResult ParseResponse(
        string body,
        DesktopItemClassificationRequest request)
    {
        ChatCompletionResponse? completion;
        try
        {
            completion = JsonSerializer.Deserialize<ChatCompletionResponse>(body, JsonOptions);
        }
        catch (JsonException ex)
        {
            return IntelligenceProviderResult.Failure(
                IntelligenceProviderStatus.InvalidResponse,
                $"Provider 外层响应不是有效 JSON：{ex.Message}");
        }

        var content = completion?.Choices?.FirstOrDefault()?.Message?.Content;
        if (string.IsNullOrWhiteSpace(content))
        {
            return IntelligenceProviderResult.Failure(
                IntelligenceProviderStatus.InvalidResponse,
                "Provider 响应缺少 choices[0].message.content。 ");
        }

        ClassificationPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<ClassificationPayload>(content, JsonOptions);
        }
        catch (JsonException ex)
        {
            return IntelligenceProviderResult.Failure(
                IntelligenceProviderStatus.InvalidResponse,
                $"Provider message.content 不是有效分类 JSON：{ex.Message}");
        }

        if (payload is null ||
            string.IsNullOrWhiteSpace(payload.Category) ||
            string.IsNullOrWhiteSpace(payload.Reason) ||
            payload.Confidence is null ||
            double.IsNaN(payload.Confidence.Value) ||
            double.IsInfinity(payload.Confidence.Value) ||
            payload.Confidence.Value is < 0d or > 1d)
        {
            return IntelligenceProviderResult.Failure(
                IntelligenceProviderStatus.InvalidResponse,
                "Provider 分类 JSON 缺少必要字段或字段范围无效。");
        }

        Guid? suggestedZoneId = null;
        if (payload.SuggestedZoneId is not null)
        {
            if (!Guid.TryParse(payload.SuggestedZoneId, out var parsedZoneId) || parsedZoneId == Guid.Empty)
            {
                return IntelligenceProviderResult.Failure(
                    IntelligenceProviderStatus.InvalidResponse,
                    "Provider suggested_zone_id 不是有效非空 GUID。");
            }

            if (!request.Zones.Any(zone => zone.ZoneId == parsedZoneId))
            {
                return IntelligenceProviderResult.Failure(
                    IntelligenceProviderStatus.InvalidResponse,
                    "Provider 推荐了当前请求中不存在的 ZoneId。");
            }

            suggestedZoneId = parsedZoneId;
        }

        try
        {
            var suggestion = new DesktopItemClassificationSuggestion(
                payload.Category,
                suggestedZoneId,
                payload.Confidence.Value,
                payload.Reason,
                DesktopItemClassificationSource.AiProvider);
            return IntelligenceProviderResult.Success(suggestion);
        }
        catch (ArgumentException ex)
        {
            return IntelligenceProviderResult.Failure(
                IntelligenceProviderStatus.InvalidResponse,
                $"Provider 分类结果未通过 Core 契约校验：{ex.Message}");
        }
    }

    private static async Task<string?> ReadLimitedUtf8Async(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > MaxResponseBytes)
            return null;

        await using var stream = await content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var buffer = new MemoryStream(capacity: 8 * 1024);
        var chunk = new byte[8 * 1024];
        var total = 0;

        while (true)
        {
            var read = await stream.ReadAsync(chunk.AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
                break;

            total += read;
            if (total > MaxResponseBytes)
                return null;

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken)
                .ConfigureAwait(false);
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, total);
    }

    private sealed class ChatCompletionResponse
    {
        [JsonPropertyName("choices")]
        public List<ChatChoice>? Choices { get; init; }
    }

    private sealed class ChatChoice
    {
        [JsonPropertyName("message")]
        public ChatMessage? Message { get; init; }
    }

    private sealed class ChatMessage
    {
        [JsonPropertyName("content")]
        public string? Content { get; init; }
    }

    private sealed class ClassificationPayload
    {
        [JsonPropertyName("category")]
        public string? Category { get; init; }

        [JsonPropertyName("suggested_zone_id")]
        public string? SuggestedZoneId { get; init; }

        [JsonPropertyName("confidence")]
        public double? Confidence { get; init; }

        [JsonPropertyName("reason")]
        public string? Reason { get; init; }
    }
}
