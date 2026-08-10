using DesktopAgent.Core.Intelligence;

namespace DesktopAgent.Application.Intelligence;

/// <summary>
/// 首次桌面理解能力边界。Provider 只产出语义草案，不表达坐标、Shell 副作用或 Writer 命令。
/// </summary>
public interface IDesktopFirstRunIntelligenceProvider
{
    Task<DesktopFirstRunUnderstandingResult> UnderstandAsync(
        DesktopFirstRunUnderstandingRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>一次首次桌面理解调用的结构化结果。</summary>
public sealed class DesktopFirstRunUnderstandingResult
{
    public IntelligenceProviderStatus Status { get; }
    public DesktopUnderstandingDraft? Draft { get; }
    public string? DiagnosticMessage { get; }
    public int? HttpStatusCode { get; }

    private DesktopFirstRunUnderstandingResult(
        IntelligenceProviderStatus status,
        DesktopUnderstandingDraft? draft,
        string? diagnosticMessage,
        int? httpStatusCode)
    {
        if (!Enum.IsDefined(status))
            throw new ArgumentOutOfRangeException(nameof(status));
        if (status == IntelligenceProviderStatus.Success && draft is null)
            throw new ArgumentException("Success 结果必须包含 Draft。", nameof(draft));
        if (status != IntelligenceProviderStatus.Success && draft is not null)
            throw new ArgumentException("非 Success 结果不能携带 Draft。", nameof(draft));
        if (httpStatusCode is < 100 or > 599)
            throw new ArgumentOutOfRangeException(nameof(httpStatusCode));

        Status = status;
        Draft = draft;
        DiagnosticMessage = diagnosticMessage;
        HttpStatusCode = httpStatusCode;
    }

    public static DesktopFirstRunUnderstandingResult Success(DesktopUnderstandingDraft draft)
        => new(
            IntelligenceProviderStatus.Success,
            draft ?? throw new ArgumentNullException(nameof(draft)),
            null,
            null);

    public static DesktopFirstRunUnderstandingResult Failure(
        IntelligenceProviderStatus status,
        string? diagnosticMessage = null,
        int? httpStatusCode = null)
    {
        if (status == IntelligenceProviderStatus.Success)
            throw new ArgumentException("Failure 不能使用 Success 状态。", nameof(status));

        return new DesktopFirstRunUnderstandingResult(status, null, diagnosticMessage, httpStatusCode);
    }
}
