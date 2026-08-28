using System.Text.Json;
using System.Text.Json.Serialization;
using DesktopAgent.Core.Intelligence;

namespace DesktopAgent.Persistence.Intelligence;

/// <summary>
/// DesktopSemanticUnderstanding 的持久化 JSON v1。
/// 使用显式 DTO 重建领域对象，避免持久化格式偶然绑定到 Core 构造器/属性反射行为。
/// </summary>
internal static class DesktopSemanticUnderstandingJsonSerializer
{
    internal const int CurrentPayloadFormatVersion = 1;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    internal static string Serialize(DesktopSemanticUnderstanding understanding)
    {
        ArgumentNullException.ThrowIfNull(understanding);

        var payload = new UnderstandingPayload
        {
            DesktopSummary = understanding.DesktopSummary,
            Categories = understanding.Categories
                .Select(static category => new CategoryPayload
                {
                    Key = category.Key,
                    Name = category.Name,
                    Description = category.Description,
                })
                .ToList(),
            Assignments = understanding.Assignments
                .Select(static assignment => new AssignmentPayload
                {
                    StableId = assignment.StableId,
                    CategoryKey = assignment.CategoryKey,
                    Confidence = assignment.Confidence,
                    Reason = assignment.Reason,
                })
                .ToList(),
            Questions = understanding.Questions
                .Select(static question => new QuestionPayload
                {
                    Key = question.Key,
                    Text = question.Text,
                })
                .ToList(),
        };

        return JsonSerializer.Serialize(payload, Options);
    }

    internal static DesktopSemanticUnderstanding Deserialize(
        string json,
        int payloadFormatVersion)
    {
        if (payloadFormatVersion != CurrentPayloadFormatVersion)
        {
            throw new InvalidDataException(
                $"不支持的 DesktopSemanticUnderstanding payload_format_version={payloadFormatVersion}；" +
                $"当前仅支持 {CurrentPayloadFormatVersion}。");
        }
        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidDataException("DesktopSemanticUnderstanding payload_json 不能为空。");

        UnderstandingPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<UnderstandingPayload>(json, Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("DesktopSemanticUnderstanding payload_json 不是有效 JSON。", ex);
        }

        if (payload is null)
            throw new InvalidDataException("DesktopSemanticUnderstanding payload_json 反序列化返回 null。");
        if (payload.Categories is null)
            throw new InvalidDataException("DesktopSemanticUnderstanding payload 缺少 categories。");
        if (payload.Assignments is null)
            throw new InvalidDataException("DesktopSemanticUnderstanding payload 缺少 assignments。");
        if (payload.Questions is null)
            throw new InvalidDataException("DesktopSemanticUnderstanding payload 缺少 questions。");

        try
        {
            var categories = payload.Categories
                .Select(static category => category is null
                    ? throw new InvalidDataException("DesktopSemanticUnderstanding categories 不能包含 null。")
                    : new DesktopSemanticCategory(
                        category.Key ?? string.Empty,
                        category.Name ?? string.Empty,
                        category.Description ?? string.Empty))
                .ToArray();

            var assignments = payload.Assignments
                .Select(static assignment => assignment is null
                    ? throw new InvalidDataException("DesktopSemanticUnderstanding assignments 不能包含 null。")
                    : new DesktopSemanticAssignment(
                        assignment.StableId ?? string.Empty,
                        assignment.CategoryKey ?? string.Empty,
                        assignment.Confidence ?? double.NaN,
                        assignment.Reason ?? string.Empty))
                .ToArray();

            var questions = payload.Questions
                .Select(static question => question is null
                    ? throw new InvalidDataException("DesktopSemanticUnderstanding questions 不能包含 null。")
                    : new DesktopSemanticQuestion(
                        question.Key ?? string.Empty,
                        question.Text ?? string.Empty))
                .ToArray();

            return new DesktopSemanticUnderstanding(
                payload.DesktopSummary ?? string.Empty,
                categories,
                assignments,
                questions);
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (ArgumentException ex)
        {
            throw new InvalidDataException(
                $"DesktopSemanticUnderstanding payload 未通过领域校验：{ex.Message}", ex);
        }
    }

    private sealed class UnderstandingPayload
    {
        [JsonPropertyName("desktop_summary")]
        public string? DesktopSummary { get; init; }

        [JsonPropertyName("categories")]
        public List<CategoryPayload?>? Categories { get; init; }

        [JsonPropertyName("assignments")]
        public List<AssignmentPayload?>? Assignments { get; init; }

        [JsonPropertyName("questions")]
        public List<QuestionPayload?>? Questions { get; init; }
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

    private sealed class AssignmentPayload
    {
        [JsonPropertyName("stable_id")]
        public string? StableId { get; init; }

        [JsonPropertyName("category_key")]
        public string? CategoryKey { get; init; }

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
