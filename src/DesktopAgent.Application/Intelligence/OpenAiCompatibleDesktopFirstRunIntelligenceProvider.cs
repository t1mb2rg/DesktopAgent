using System.Text.Json;
using System.Text.Json.Serialization;
using DesktopAgent.Core.Intelligence;

namespace DesktopAgent.Application.Intelligence;

/// <summary>
/// 使用共享 OpenAI-compatible transport 完成首次整桌语义理解。
/// 对外发送的项目和已有 Zone 都使用单轮临时引用，StableId 与真实 ZoneId 保留在本地映射中。
/// </summary>
public sealed class OpenAiCompatibleDesktopFirstRunIntelligenceProvider : IDesktopFirstRunIntelligenceProvider
{
    private const int MaxCategories = 16;
    private const int MaxZones = 16;
    private const int MaxQuestions = 3;
    private const int MaxDesktopSummaryChars = 4096;
    private const int MaxKeyChars = 64;
    private const int MaxNameChars = 128;
    private const int MaxDescriptionChars = 512;
    private const int MaxReasonChars = 1024;
    private const int MaxQuestionChars = 512;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private const string SystemPrompt = """
You are the first-run desktop understanding component of DesktopAgent.
Your job is to understand how this user wants their native Windows desktop organized.
Be proactive: infer useful semantic categories, propose logical zones, assign items when reasonably confident, and ask at most 3 concise clarifying questions when user intent is still ambiguous.

Return JSON only. Never output physical pixel coordinates, normalized rectangles, shell commands, file move/delete commands, scripts, or executable actions.
Only use the temporary item_ref and existing_zone_ref values supplied in this request. They are opaque request-local references.
Do not invent a new item_ref or existing_zone_ref.

placement_hint must be one of:
unspecified, left, right, top, bottom, center, top_left, top_right, bottom_left, bottom_right.
size_hint must be one of:
flexible, compact, medium, large.

Return this semantic shape:
{
  "intent_summary": "bounded summary of what DesktopAgent currently understands about the user's organization intent",
  "desktop_summary": "concise description of the current desktop and proposed organization",
  "categories": [
    { "key": "stable-semantic-key", "name": "user-visible name", "description": "concise meaning" }
  ],
  "zones": [
    {
      "key": "logical-zone-key",
      "name": "user-visible zone name",
      "reuse_existing_zone_ref": "existing-zone-0001 or null",
      "placement_hint": "right",
      "size_hint": "large",
      "category_keys": ["category-key"]
    }
  ],
  "assignments": [
    {
      "item_ref": "item-0001",
      "category_key": "category-key",
      "zone_key": "logical-zone-key or null",
      "confidence": 0.9,
      "reason": "concise explanation"
    }
  ],
  "questions": [
    { "key": "question-key", "text": "one concise question for the user" }
  ]
}

Keys must be unique inside their collection. Category and zone references must refer to objects returned in the same response. It is valid to leave uncertain items unassigned and ask the user instead.
""";

    private readonly OpenAiCompatibleJsonChatTransport _transport;

    public OpenAiCompatibleDesktopFirstRunIntelligenceProvider(
        HttpClient httpClient,
        OpenAiCompatibleProviderOptions options,
        IIntelligenceSecretResolver secretResolver)
    {
        _transport = new OpenAiCompatibleJsonChatTransport(httpClient, options, secretResolver);
    }

    public async Task<DesktopFirstRunUnderstandingResult> UnderstandAsync(
        DesktopFirstRunUnderstandingRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var itemRefs = request.Items
            .Select(static (item, index) => new ItemReference($"item-{index + 1:D4}", item))
            .ToArray();
        var existingZoneRefs = request.ExistingZones
            .Select(static (zone, index) => new ExistingZoneReference($"existing-zone-{index + 1:D4}", zone))
            .ToArray();

        var context = new
        {
            previous_intent_summary = request.CurrentIntentSummary,
            latest_user_guidance = request.LatestUserGuidance,
            items = itemRefs.Select(static entry => new
            {
                item_ref = entry.Reference,
                display_name = entry.Item.DisplayName,
                file_extension = entry.Item.FileExtension,
                item_kind = entry.Item.ItemKind.ToString(),
                is_system_item = entry.Item.IsSystemItem,
                is_file_system_item = entry.Item.IsFileSystemItem,
            }),
            existing_zones = existingZoneRefs.Select(static entry => new
            {
                existing_zone_ref = entry.Reference,
                name = entry.Zone.Name,
            }),
        };

        var transportResult = await _transport.CompleteJsonAsync(
                SystemPrompt,
                "Understand this desktop and the user's current intent. Input JSON:\n" +
                JsonSerializer.Serialize(context, JsonOptions),
                cancellationToken)
            .ConfigureAwait(false);

        if (transportResult.Status != IntelligenceProviderStatus.Success)
        {
            return DesktopFirstRunUnderstandingResult.Failure(
                transportResult.Status,
                transportResult.DiagnosticMessage,
                transportResult.HttpStatusCode);
        }

        return ParseResponse(transportResult.Content!, request, itemRefs, existingZoneRefs);
    }

    private static DesktopFirstRunUnderstandingResult ParseResponse(
        string content,
        DesktopFirstRunUnderstandingRequest request,
        IReadOnlyList<ItemReference> itemRefs,
        IReadOnlyList<ExistingZoneReference> existingZoneRefs)
    {
        UnderstandingPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<UnderstandingPayload>(content, JsonOptions);
        }
        catch (JsonException ex)
        {
            return Invalid($"Provider message.content 不是有效首次理解 JSON：{ex.Message}");
        }

        if (payload is null ||
            !HasTextWithin(payload.IntentSummary, DesktopFirstRunUnderstandingRequest.MaxIntentSummaryChars) ||
            !HasTextWithin(payload.DesktopSummary, MaxDesktopSummaryChars) ||
            payload.Categories is null ||
            payload.Categories.Count is < 1 or > MaxCategories ||
            (payload.Zones?.Count ?? 0) > MaxZones ||
            (payload.Assignments?.Count ?? 0) > request.Items.Count ||
            (payload.Questions?.Count ?? 0) > MaxQuestions)
        {
            return Invalid("Provider 首次理解 JSON 缺少必要字段或集合/文本超过 v1.0 上限。");
        }

        var itemMap = itemRefs.ToDictionary(static entry => entry.Reference, StringComparer.Ordinal);
        var existingZoneMap = existingZoneRefs.ToDictionary(static entry => entry.Reference, StringComparer.Ordinal);

        try
        {
            var categories = payload.Categories.Select(category =>
            {
                RequireTextWithin(category.Key, MaxKeyChars, "category.key");
                RequireTextWithin(category.Name, MaxNameChars, "category.name");
                RequireTextWithin(category.Description, MaxDescriptionChars, "category.description");
                return new DesktopUnderstandingCategoryDraft(
                    category.Key!,
                    category.Name!,
                    category.Description!);
            }).ToArray();

            var zones = (payload.Zones ?? []).Select(zone =>
            {
                RequireTextWithin(zone.Key, MaxKeyChars, "zone.key");
                RequireTextWithin(zone.Name, MaxNameChars, "zone.name");

                if (!TryParsePlacementHint(zone.PlacementHint, out var placementHint))
                    throw new InvalidDataException("zone.placement_hint 不在允许枚举中。");
                if (!TryParseSizeHint(zone.SizeHint, out var sizeHint))
                    throw new InvalidDataException("zone.size_hint 不在允许枚举中。");

                Guid? existingZoneId = null;
                if (zone.ReuseExistingZoneRef is not null)
                {
                    if (!existingZoneMap.TryGetValue(zone.ReuseExistingZoneRef, out var existingEntry))
                        throw new InvalidDataException("Zone 引用了请求中不存在的 existing_zone_ref。");
                    existingZoneId = existingEntry.Zone.ZoneId;
                }

                var categoryKeys = zone.CategoryKeys ?? [];
                foreach (var categoryKey in categoryKeys)
                    RequireTextWithin(categoryKey, MaxKeyChars, "zone.category_keys[]");

                return new DesktopUnderstandingZoneDraft(
                    zone.Key!,
                    zone.Name!,
                    existingZoneId,
                    placementHint,
                    sizeHint,
                    categoryKeys!);
            }).ToArray();

            var assignments = (payload.Assignments ?? []).Select(assignment =>
            {
                RequireTextWithin(assignment.ItemRef, MaxKeyChars, "assignment.item_ref");
                RequireTextWithin(assignment.CategoryKey, MaxKeyChars, "assignment.category_key");
                if (assignment.ZoneKey is not null)
                    RequireTextWithin(assignment.ZoneKey, MaxKeyChars, "assignment.zone_key");
                RequireTextWithin(assignment.Reason, MaxReasonChars, "assignment.reason");
                if (assignment.Confidence is null ||
                    !double.IsFinite(assignment.Confidence.Value) ||
                    assignment.Confidence.Value is < 0d or > 1d)
                {
                    throw new InvalidDataException("assignment.confidence 必须位于 [0, 1]。");
                }

                if (!itemMap.TryGetValue(assignment.ItemRef!, out var itemEntry))
                    throw new InvalidDataException("Assignment 引用了请求中不存在的 item_ref。");

                return new DesktopUnderstandingAssignment(
                    itemEntry.Item.StableId,
                    assignment.CategoryKey!,
                    assignment.ZoneKey,
                    assignment.Confidence.Value,
                    assignment.Reason!);
            }).ToArray();

            var questions = (payload.Questions ?? []).Select(question =>
            {
                RequireTextWithin(question.Key, MaxKeyChars, "question.key");
                RequireTextWithin(question.Text, MaxQuestionChars, "question.text");
                return new DesktopUnderstandingQuestion(question.Key!, question.Text!);
            }).ToArray();

            var draft = new DesktopUnderstandingDraft(
                payload.IntentSummary!,
                payload.DesktopSummary!,
                categories,
                zones,
                assignments,
                questions);
            return DesktopFirstRunUnderstandingResult.Success(draft);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException)
        {
            return Invalid($"Provider 首次理解结果未通过本地语义校验：{ex.Message}");
        }
    }

    private static void RequireTextWithin(string? value, int maxChars, string field)
    {
        if (!HasTextWithin(value, maxChars))
            throw new InvalidDataException($"{field} 为空或超过 {maxChars} 字符上限。");
    }

    private static bool HasTextWithin(string? value, int maxChars)
        => !string.IsNullOrWhiteSpace(value) && value.Length <= maxChars;

    private static bool TryParsePlacementHint(string? value, out DesktopZonePlacementHint result)
    {
        result = value?.Trim().ToLowerInvariant() switch
        {
            "unspecified" => DesktopZonePlacementHint.Unspecified,
            "left" => DesktopZonePlacementHint.Left,
            "right" => DesktopZonePlacementHint.Right,
            "top" => DesktopZonePlacementHint.Top,
            "bottom" => DesktopZonePlacementHint.Bottom,
            "center" => DesktopZonePlacementHint.Center,
            "top_left" => DesktopZonePlacementHint.TopLeft,
            "top_right" => DesktopZonePlacementHint.TopRight,
            "bottom_left" => DesktopZonePlacementHint.BottomLeft,
            "bottom_right" => DesktopZonePlacementHint.BottomRight,
            _ => (DesktopZonePlacementHint)(-1),
        };
        return Enum.IsDefined(result);
    }

    private static bool TryParseSizeHint(string? value, out DesktopZoneSizeHint result)
    {
        result = value?.Trim().ToLowerInvariant() switch
        {
            "flexible" => DesktopZoneSizeHint.Flexible,
            "compact" => DesktopZoneSizeHint.Compact,
            "medium" => DesktopZoneSizeHint.Medium,
            "large" => DesktopZoneSizeHint.Large,
            _ => (DesktopZoneSizeHint)(-1),
        };
        return Enum.IsDefined(result);
    }

    private static DesktopFirstRunUnderstandingResult Invalid(string message)
        => DesktopFirstRunUnderstandingResult.Failure(
            IntelligenceProviderStatus.InvalidResponse,
            message);

    private sealed record ItemReference(string Reference, DesktopItemFacts Item);
    private sealed record ExistingZoneReference(string Reference, IntelligenceZoneOption Zone);

    private sealed class UnderstandingPayload
    {
        [JsonPropertyName("intent_summary")]
        public string? IntentSummary { get; init; }

        [JsonPropertyName("desktop_summary")]
        public string? DesktopSummary { get; init; }

        [JsonPropertyName("categories")]
        public List<CategoryPayload>? Categories { get; init; }

        [JsonPropertyName("zones")]
        public List<ZonePayload>? Zones { get; init; }

        [JsonPropertyName("assignments")]
        public List<AssignmentPayload>? Assignments { get; init; }

        [JsonPropertyName("questions")]
        public List<QuestionPayload>? Questions { get; init; }
    }

    private sealed class CategoryPayload
    {
        [JsonPropertyName("key")]
        public string? Key { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("description")]
        public string? Description { get; init; }
    }

    private sealed class ZonePayload
    {
        [JsonPropertyName("key")]
        public string? Key { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("reuse_existing_zone_ref")]
        public string? ReuseExistingZoneRef { get; init; }

        [JsonPropertyName("placement_hint")]
        public string? PlacementHint { get; init; }

        [JsonPropertyName("size_hint")]
        public string? SizeHint { get; init; }

        [JsonPropertyName("category_keys")]
        public List<string>? CategoryKeys { get; init; }
    }

    private sealed class AssignmentPayload
    {
        [JsonPropertyName("item_ref")]
        public string? ItemRef { get; init; }

        [JsonPropertyName("category_key")]
        public string? CategoryKey { get; init; }

        [JsonPropertyName("zone_key")]
        public string? ZoneKey { get; init; }

        [JsonPropertyName("confidence")]
        public double? Confidence { get; init; }

        [JsonPropertyName("reason")]
        public string? Reason { get; init; }
    }

    private sealed class QuestionPayload
    {
        [JsonPropertyName("key")]
        public string? Key { get; init; }

        [JsonPropertyName("text")]
        public string? Text { get; init; }
    }
}
