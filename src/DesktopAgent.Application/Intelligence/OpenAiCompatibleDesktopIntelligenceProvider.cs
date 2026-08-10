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

    private readonly OpenAiCompatibleJsonChatTransport _transport;

    public OpenAiCompatibleDesktopIntelligenceProvider(
        HttpClient httpClient,
        OpenAiCompatibleProviderOptions options,
        IIntelligenceSecretResolver secretResolver)
    {
        _transport = new OpenAiCompatibleJsonChatTransport(httpClient, options, secretResolver);
    }

    public async Task<IntelligenceProviderResult> ClassifyAsync(
        DesktopItemClassificationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

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
            }),
        };

        var transportResult = await _transport.CompleteJsonAsync(
                SystemPrompt,
                "Classify this desktop item. Input JSON:\n" +
                JsonSerializer.Serialize(itemContext, JsonOptions),
                cancellationToken)
            .ConfigureAwait(false);

        if (transportResult.Status != IntelligenceProviderStatus.Success)
        {
            return IntelligenceProviderResult.Failure(
                transportResult.Status,
                transportResult.DiagnosticMessage,
                transportResult.HttpStatusCode);
        }

        return ParseResponse(transportResult.Content!, request);
    }

    private static IntelligenceProviderResult ParseResponse(
        string content,
        DesktopItemClassificationRequest request)
    {
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
            !double.IsFinite(payload.Confidence.Value) ||
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
