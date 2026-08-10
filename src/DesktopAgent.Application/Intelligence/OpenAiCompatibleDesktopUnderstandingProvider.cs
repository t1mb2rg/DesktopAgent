using System.Text.Json;
using System.Text.Json.Serialization;
using DesktopAgent.Core.Intelligence;

namespace DesktopAgent.Application.Intelligence;

/// <summary>
/// 使用 OpenAI-compatible Provider 对首次桌面做整体语义理解。
/// 返回可确认的 Zone / assignment 草案，不返回物理坐标或执行命令。
/// </summary>
public sealed class OpenAiCompatibleDesktopUnderstandingProvider : IDesktopUnderstandingProvider
{
    private const int MaxDisplayNameLength = 512;
    private const int MaxExtensionLength = 64;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private const string SystemPrompt = """
You are the first-run desktop understanding component of DesktopAgent.
Understand the user's current desktop organization intent and propose a simple logical layout.
Return JSON only. Never output physical coordinates, normalized coordinate numbers, shell commands, file move/delete commands, scripts, or executable actions.
You may choose only one layout_pattern from: Grid, Columns, Rows, PrimaryLeft, PrimaryRight, PrimaryTop, PrimaryBottom.
The first zone is the primary zone for Primary* patterns.
Create at most 8 logical zones. Zone key must contain only ASCII letters, digits, '-' or '_'. Zone names and purposes may use the user's language.
Every supplied item_ref must be assigned exactly once to one proposed zone. If uncertain, create or use a reasonable fallback zone rather than omitting the item.
You may return at most 3 concise clarifying questions, but still produce a complete provisional proposal.
The JSON shape is:
{
  "summary": "non-empty explanation",
  "layout_pattern": "Grid",
  "zones": [
    {"key":"development","name":"开发","purpose":"development tools and projects"}
  ],
  "assignments": [
    {"item_ref":"i1","zone_key":"development"}
  ],
  "questions": [],
  "confidence": 0.0
}
confidence must be between 0 and 1 inclusive.
""";

    private readonly OpenAiCompatibleChatClient _chatClient;

    public OpenAiCompatibleDesktopUnderstandingProvider(
        HttpClient httpClient,
        OpenAiCompatibleProviderOptions options,
        IIntelligenceSecretResolver secretResolver)
    {
        _chatClient = new OpenAiCompatibleChatClient(
            httpClient ?? throw new ArgumentNullException(nameof(httpClient)),
            options ?? throw new ArgumentNullException(nameof(options)),
            secretResolver ?? throw new ArgumentNullException(nameof(secretResolver)));
    }

    public async Task<DesktopUnderstandingProviderResult> UnderstandAsync(
        DesktopUnderstandingRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var chatResult = await _chatClient.CompleteJsonAsync(
                SystemPrompt,
                BuildUserPrompt(request),
                cancellationToken)
            .ConfigureAwait(false);

        if (chatResult.Status != IntelligenceProviderStatus.Success)
        {
            return DesktopUnderstandingProviderResult.Failure(
                chatResult.Status,
                chatResult.DiagnosticMessage,
                chatResult.HttpStatusCode);
        }

        return ParseProposal(chatResult.Content!, request);
    }

    private static string BuildUserPrompt(DesktopUnderstandingRequest request)
    {
        var context = new
        {
            user_intent = request.UserIntent,
            items = request.Items.Select(static (item, index) => new
            {
                item_ref = $"i{index + 1}",
                display_name = Limit(item.DisplayName, MaxDisplayNameLength),
                file_extension = Limit(item.FileExtension, MaxExtensionLength),
                item_kind = item.ItemKind.ToString(),
                is_system_item = item.IsSystemItem,
                is_file_system_item = item.IsFileSystemItem,
            }),
        };

        return "Propose the first DesktopAgent organization draft for this desktop. Input JSON:\n" +
               JsonSerializer.Serialize(context, JsonOptions);
    }

    private static DesktopUnderstandingProviderResult ParseProposal(
        string content,
        DesktopUnderstandingRequest request)
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
            string.IsNullOrWhiteSpace(payload.Summary) ||
            payload.Summary.Length > DesktopUnderstandingProposal.MaxSummaryLength ||
            payload.Confidence is null ||
            !double.IsFinite(payload.Confidence.Value) ||
            payload.Confidence.Value is < 0d or > 1d ||
            payload.Zones is null ||
            payload.Assignments is null)
        {
            return Invalid("Provider 首次理解 JSON 缺少必要字段或字段范围无效。");
        }

        if (!TryParsePattern(payload.LayoutPattern, out var pattern))
            return Invalid("Provider layout_pattern 不属于 DesktopAgent 允许的有限布局模式。");

        if (payload.Zones.Count is < 1 or > DesktopUnderstandingProposal.MaxZoneCount)
            return Invalid($"Provider zones 数量必须在 1..{DesktopUnderstandingProposal.MaxZoneCount}。");
        if (payload.Assignments.Count != request.Items.Count)
            return Invalid("Provider 必须为每个输入 item_ref 恰好返回一个 assignment。");
        if (payload.Questions is { Count: > DesktopUnderstandingProposal.MaxQuestionCount })
            return Invalid($"Provider questions 不能超过 {DesktopUnderstandingProposal.MaxQuestionCount} 条。");

        try
        {
            var zones = payload.Zones.Select(static zone =>
                    new DesktopUnderstandingZoneProposal(
                        zone.Key ?? "",
                        zone.Name ?? "",
                        zone.Purpose ?? ""))
                .ToArray();

            var assignments = payload.Assignments.Select(static assignment =>
                    new DesktopUnderstandingItemAssignment(
                        assignment.ItemRef ?? "",
                        assignment.ZoneKey ?? ""))
                .ToArray();

            var knownRefs = Enumerable.Range(1, request.Items.Count)
                .Select(static index => $"i{index}")
                .ToHashSet(StringComparer.Ordinal);
            if (assignments.Any(assignment => !knownRefs.Contains(assignment.ItemRef)))
                return Invalid("Provider assignment 引用了当前请求中不存在的 item_ref。");

            var proposal = new DesktopUnderstandingProposal(
                payload.Summary,
                pattern,
                zones,
                assignments,
                payload.Questions ?? Array.Empty<string>(),
                payload.Confidence.Value);

            if (proposal.Assignments.Count != knownRefs.Count ||
                proposal.Assignments.Any(assignment => !knownRefs.Contains(assignment.ItemRef)))
            {
                return Invalid("Provider 没有对全部 item_ref 形成一一对应的完整 assignment。");
            }

            return DesktopUnderstandingProviderResult.Success(proposal);
        }
        catch (ArgumentException ex)
        {
            return Invalid($"Provider 首次理解结果未通过本地契约校验：{ex.Message}");
        }
    }

    private static bool TryParsePattern(string? value, out DesktopZoneLayoutPattern pattern)
    {
        pattern = value switch
        {
            "Grid" => DesktopZoneLayoutPattern.Grid,
            "Columns" => DesktopZoneLayoutPattern.Columns,
            "Rows" => DesktopZoneLayoutPattern.Rows,
            "PrimaryLeft" => DesktopZoneLayoutPattern.PrimaryLeft,
            "PrimaryRight" => DesktopZoneLayoutPattern.PrimaryRight,
            "PrimaryTop" => DesktopZoneLayoutPattern.PrimaryTop,
            "PrimaryBottom" => DesktopZoneLayoutPattern.PrimaryBottom,
            _ => default,
        };
        return value is "Grid" or "Columns" or "Rows" or
            "PrimaryLeft" or "PrimaryRight" or "PrimaryTop" or "PrimaryBottom";
    }

    private static DesktopUnderstandingProviderResult Invalid(string diagnostic)
        => DesktopUnderstandingProviderResult.Failure(
            IntelligenceProviderStatus.InvalidResponse,
            diagnostic);

    private static string? Limit(string? value, int maxLength)
        => value is null || value.Length <= maxLength
            ? value
            : value[..maxLength];

    private sealed class UnderstandingPayload
    {
        [JsonPropertyName("summary")]
        public string? Summary { get; init; }

        [JsonPropertyName("layout_pattern")]
        public string? LayoutPattern { get; init; }

        [JsonPropertyName("zones")]
        public List<ZonePayload>? Zones { get; init; }

        [JsonPropertyName("assignments")]
        public List<AssignmentPayload>? Assignments { get; init; }

        [JsonPropertyName("questions")]
        public List<string>? Questions { get; init; }

        [JsonPropertyName("confidence")]
        public double? Confidence { get; init; }
    }

    private sealed class ZonePayload
    {
        [JsonPropertyName("key")]
        public string? Key { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("purpose")]
        public string? Purpose { get; init; }
    }

    private sealed class AssignmentPayload
    {
        [JsonPropertyName("item_ref")]
        public string? ItemRef { get; init; }

        [JsonPropertyName("zone_key")]
        public string? ZoneKey { get; init; }
    }
}
