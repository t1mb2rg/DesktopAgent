using DesktopAgent.Core.Intelligence;

namespace DesktopAgent.Application.Intelligence;

/// <summary>
/// 一次智能 Provider 调用的结构化结果。
/// Success 才携带 Suggestion；失败状态只提供诊断信息，不向下游伪造分类决定。
/// </summary>
public sealed class IntelligenceProviderResult
{
    public IntelligenceProviderStatus Status { get; }
    public DesktopItemClassificationSuggestion? Suggestion { get; }
    public string? DiagnosticMessage { get; }
    public int? HttpStatusCode { get; }

    private IntelligenceProviderResult(
        IntelligenceProviderStatus status,
        DesktopItemClassificationSuggestion? suggestion,
        string? diagnosticMessage,
        int? httpStatusCode)
    {
        if (!Enum.IsDefined(status))
            throw new ArgumentOutOfRangeException(nameof(status));
        if (status == IntelligenceProviderStatus.Success && suggestion is null)
            throw new ArgumentException("Success 结果必须包含 Suggestion。", nameof(suggestion));
        if (status != IntelligenceProviderStatus.Success && suggestion is not null)
            throw new ArgumentException("非 Success 结果不能携带 Suggestion。", nameof(suggestion));
        if (httpStatusCode is < 100 or > 599)
            throw new ArgumentOutOfRangeException(nameof(httpStatusCode));

        Status = status;
        Suggestion = suggestion;
        DiagnosticMessage = diagnosticMessage;
        HttpStatusCode = httpStatusCode;
    }

    public static IntelligenceProviderResult Success(DesktopItemClassificationSuggestion suggestion)
        => new(IntelligenceProviderStatus.Success,
            suggestion ?? throw new ArgumentNullException(nameof(suggestion)), null, null);

    public static IntelligenceProviderResult Failure(
        IntelligenceProviderStatus status,
        string? diagnosticMessage = null,
        int? httpStatusCode = null)
    {
        if (status == IntelligenceProviderStatus.Success)
            throw new ArgumentException("Failure 不能使用 Success 状态。", nameof(status));

        return new IntelligenceProviderResult(status, null, diagnosticMessage, httpStatusCode);
    }
}
